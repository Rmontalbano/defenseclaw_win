namespace DefenseClaw.App.Services;

/// <summary>One line of the shortcuts overlay: the chord and what it does.</summary>
/// <param name="Keys">Display text such as <c>Ctrl+Shift+1</c>; the overlay splits it into key caps.</param>
/// <param name="Description">What the chord does.</param>
internal sealed record ShortcutRow(string Keys, string Description)
{
    /// <summary>The key parts, one per cap (<c>Ctrl</c>, <c>Shift</c>, <c>1</c>).</summary>
    public IReadOnlyList<string> Parts => Keys.Split('+');

    /// <summary>What a screen reader announces for the row: the sentence, not the record's type name.</summary>
    public override string ToString() => $"{Description}: {Keys}";
}

/// <summary>A titled group of <see cref="ShortcutRow"/>s.</summary>
internal sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutRow> Rows)
{
    public override string ToString() => Title;
}

/// <summary>What the shortcuts overlay binds to: panel chords on the left, the rest on the right.</summary>
internal sealed record ShortcutsModel(ShortcutSection Panels, IReadOnlyList<ShortcutSection> Others);

/// <summary>Builds the overlay's content from <see cref="ShellShortcuts"/> and the live panel list.</summary>
internal static class ShortcutCatalog
{
    public static ShortcutsModel Build(PanelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var panelRows = new List<ShortcutRow>();
        for (var i = 0; i < catalog.SidebarOrder.Count; i++)
        {
            if (ShellShortcuts.PanelChordText(i) is { } chord)
            {
                panelRows.Add(new ShortcutRow(chord, catalog.SidebarOrder[i].Title));
            }
        }

        var others = new List<ShortcutSection>
        {
            new(
                "Anywhere in the dashboard",
                new[]
                {
                    new ShortcutRow(ShellShortcuts.PaletteText, "Command palette: search every panel and action"),
                    new ShortcutRow(ShellShortcuts.RefreshText, "Refresh the current panel (or the gateway status)"),
                    new ShortcutRow(ShellShortcuts.ToggleThemeText, "Switch between light and dark (the title bar's Appearance button picks a style)"),
                    new ShortcutRow(ShellShortcuts.HelpText, "Show this list (also ? outside a text box)"),
                    new ShortcutRow(ShellShortcuts.CloseText, "Close the open overlay, detail pane or dialog"),
                }),
            new(
                "Inside a panel",
                new[]
                {
                    new ShortcutRow(ShellShortcuts.FindText, "Focus the panel's search or filter box, where it has one"),
                    new ShortcutRow("Tab", "Move to the next control; Shift+Tab goes back"),
                }),
        };

        return new ShortcutsModel(new ShortcutSection("Go to a panel", panelRows), others);
    }
}
