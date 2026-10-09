using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Shell;

public sealed class ShellShortcutsTests : IDisposable
{
    /// <summary>The sidebar, top to bottom. "ai-runtime" (CUST-309) sits after AI Discovery but needs a newer runtime, so it is not among the chord order's first fourteen.</summary>
    private static readonly string[] SidebarIds =
    {
        "overview", "alerts", "logs", "audit", "activity",
        "skills", "mcps", "plugins", "tools",
        "inventory", "ai-discovery", "ai-runtime", "registries",
        "setup", "policies",
    };

    /// <summary>The panels every runtime has, in sidebar order: the ones that keep their chord whatever the connected runtime can do.</summary>
    private static readonly string[] BaseIds = SidebarIds.Where(id => id != "ai-runtime").ToArray();

    private static readonly string[] ChordTexts =
    {
        "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5", "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9", "Ctrl+0",
        "Ctrl+Shift+1", "Ctrl+Shift+2", "Ctrl+Shift+3", "Ctrl+Shift+4", "Ctrl+Shift+5",
    };

    private readonly TempDirectory _temp = new();
    private readonly AppServices _services;

    public ShellShortcutsTests()
    {
        _services = TestServices.Create(_temp);
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }

    private static Key DigitKey(int digit) => Key.D0 + digit;

    // ------------------------------------------------------------------ the chord table

    [Fact]
    public void Fifteen_panels_are_numbered()
    {
        Assert.Equal(15, ShellShortcuts.NumberedPanels);
        Assert.Equal(ShellShortcuts.NumberedPanels, ChordTexts.Length);
    }

    [Fact]
    public void Chord_text_counts_ctrl_1_to_9_then_ctrl_0_then_ctrl_shift_1_to_5()
    {
        for (var i = 0; i < ChordTexts.Length; i++)
        {
            Assert.Equal(ChordTexts[i], ShellShortcuts.PanelChordText(i));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(int.MaxValue)]
    public void A_panel_past_the_fifteenth_has_no_chord(int index)
    {
        Assert.Null(ShellShortcuts.PanelChordText(index));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(6, 5)]
    [InlineData(7, 6)]
    [InlineData(8, 7)]
    [InlineData(9, 8)]
    [InlineData(0, 9)]
    public void Ctrl_and_a_digit_selects_the_panel_in_sidebar_order(int digit, int expectedIndex)
    {
        Assert.Equal(expectedIndex, ShellShortcuts.PanelIndexFor(DigitKey(digit), ModifierKeys.Control));
        Assert.Equal(expectedIndex, ShellShortcuts.PanelIndexFor(Key.NumPad0 + digit, ModifierKeys.Control));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 11)]
    [InlineData(3, 12)]
    [InlineData(4, 13)]
    [InlineData(5, 14)]
    public void Ctrl_shift_and_1_to_5_select_the_overflow_panels(int digit, int expectedIndex)
    {
        var modifiers = ModifierKeys.Control | ModifierKeys.Shift;

        Assert.Equal(expectedIndex, ShellShortcuts.PanelIndexFor(DigitKey(digit), modifiers));
        Assert.Equal(expectedIndex, ShellShortcuts.PanelIndexFor(Key.NumPad0 + digit, modifiers));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public void Ctrl_shift_with_any_other_digit_is_not_a_panel_chord(int digit)
    {
        Assert.Null(ShellShortcuts.PanelIndexFor(DigitKey(digit), ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift)]
    public void Only_exactly_ctrl_or_exactly_ctrl_shift_counts(ModifierKeys modifiers)
    {
        Assert.Null(ShellShortcuts.PanelIndexFor(Key.D1, modifiers));
        Assert.Null(ShellShortcuts.PanelIndexFor(Key.NumPad2, modifiers));
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.K)]
    [InlineData(Key.F5)]
    [InlineData(Key.OemPlus)]
    [InlineData(Key.Escape)]
    public void A_key_that_is_not_a_digit_is_never_a_panel_chord(Key key)
    {
        Assert.Null(ShellShortcuts.PanelIndexFor(key, ModifierKeys.Control));
        Assert.Null(ShellShortcuts.PanelIndexFor(key, ModifierKeys.Control | ModifierKeys.Shift));
    }

    [Fact]
    public void The_text_of_every_chord_maps_back_to_its_own_index()
    {
        for (var i = 0; i < ShellShortcuts.NumberedPanels; i++)
        {
            var parts = ShellShortcuts.PanelChordText(i)!.Split('+');
            var modifiers = parts.Contains("Shift") ? ModifierKeys.Control | ModifierKeys.Shift : ModifierKeys.Control;
            var key = DigitKey(int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture));

            Assert.Equal(i, ShellShortcuts.PanelIndexFor(key, modifiers));
        }
    }

    [Fact]
    public void The_fixed_key_texts_are_the_documented_ones()
    {
        Assert.Equal("Ctrl+K", ShellShortcuts.PaletteText);
        Assert.Equal("F5", ShellShortcuts.RefreshText);
        Assert.Equal("F1", ShellShortcuts.HelpText);
        Assert.Equal("Ctrl+F", ShellShortcuts.FindText);
        Assert.Equal("Esc", ShellShortcuts.CloseText);
    }

    // ------------------------------------------------------------------ the chords against the real sidebar

    [Fact]
    public void All_fifteen_panels_are_in_the_sidebar_and_the_chords_count_the_fourteen_every_runtime_has_first()
    {
        var catalog = new PanelCatalog(_services);

        Assert.Equal(SidebarIds, catalog.SidebarOrder.Select(p => p.Id).ToArray());
        Assert.Equal(ShellShortcuts.NumberedPanels, catalog.SidebarOrder.Count);

        // The chord order: the panels that need nothing in sidebar order (so every chord of 0.8.10 is where it was), then the one that needs a
        // newer runtime. Putting Runtime in the middle of the sidebar moved no existing chord.
        Assert.Equal(BaseIds.Append("ai-runtime").ToArray(), catalog.ChordOrder.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "ai-runtime" }, catalog.Panels.Where(p => p.Requires is not null).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Every_chord_lands_on_the_panel_at_that_position_in_the_chord_order()
    {
        var catalog = new PanelCatalog(_services);

        var byChord = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < ChordTexts.Length; i++)
        {
            var parts = ChordTexts[i].Split('+');
            var modifiers = parts.Contains("Shift") ? ModifierKeys.Control | ModifierKeys.Shift : ModifierKeys.Control;
            var index = ShellShortcuts.PanelIndexFor(DigitKey(int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture)), modifiers);
            byChord[ChordTexts[i]] = catalog.ChordOrder[index!.Value].Id;
        }

        Assert.Equal("overview", byChord["Ctrl+1"]);
        Assert.Equal("alerts", byChord["Ctrl+2"]);
        Assert.Equal("tools", byChord["Ctrl+9"]);
        Assert.Equal("inventory", byChord["Ctrl+0"]);
        Assert.Equal("ai-discovery", byChord["Ctrl+Shift+1"]);
        Assert.Equal("registries", byChord["Ctrl+Shift+2"]);
        Assert.Equal("setup", byChord["Ctrl+Shift+3"]);
        Assert.Equal("policies", byChord["Ctrl+Shift+4"]);
        Assert.Equal("ai-runtime", byChord["Ctrl+Shift+5"]);
    }

    [Fact]
    public void The_sidebar_order_is_group_by_group_and_the_catalog_lists_every_panel_once()
    {
        var catalog = new PanelCatalog(_services);

        Assert.Equal(new[] { "Monitor", "Govern", "Discover", "Configure" }, PanelCatalog.Groups.ToArray());
        Assert.Equal(
            new[] { "Monitor", "Monitor", "Monitor", "Monitor", "Monitor", "Govern", "Govern", "Govern", "Govern", "Discover", "Discover", "Discover", "Discover", "Configure", "Configure" },
            catalog.SidebarOrder.Select(p => p.Group).ToArray());
        Assert.Equal(16, catalog.Panels.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("overview", catalog.Default.Id);

        // Settings is the sixteenth: pinned below the groups, in no group, and outside the numbered order (Ctrl+, reaches it).
        Assert.Equal(new[] { "settings" }, catalog.FooterPanels.Select(p => p.Id).ToArray());
        Assert.DoesNotContain(catalog.SidebarOrder, p => p.Id == "settings");
        Assert.Equal(PanelCatalog.FooterGroup, catalog.ById("settings")!.Group);
    }

    [Fact]
    public void Panels_are_found_by_id_ignoring_case_and_by_unknown_id_not_at_all()
    {
        var catalog = new PanelCatalog(_services);

        Assert.Equal("Registries", catalog.ById("REGISTRIES")?.Title);
        Assert.Null(catalog.ById("nope"));
    }

    [Fact]
    public void The_shortcuts_overlay_lists_a_row_per_panel_on_offer_with_the_same_chords()
    {
        var catalog = new PanelCatalog(_services);

        var model = ShortcutCatalog.Build(catalog);

        // An unprobed runtime offers the fourteen panels 0.8.10 has: the overlay must not promise a chord that does nothing.
        Assert.Equal(14, model.Panels.Rows.Count);
        Assert.Equal(ChordTexts.Take(14).ToArray(), model.Panels.Rows.Select(r => r.Keys).ToArray());
        Assert.Equal(catalog.ChordOrder.Where(p => p.Requires is null).Select(p => p.Title).ToArray(), model.Panels.Rows.Select(r => r.Description).ToArray());
        Assert.DoesNotContain(model.Panels.Rows, r => r.Description == "Runtime");
        Assert.Equal(new[] { "Ctrl", "Shift", "1" }, model.Panels.Rows[10].Parts);
        Assert.Equal("Alerts: Ctrl+2", model.Panels.Rows[1].ToString());
    }

    [Fact]
    public void The_palette_lists_a_go_to_command_per_panel_carrying_its_chord_and_navigating_there()
    {
        var catalog = new PanelCatalog(_services);
        var navigated = new List<string>();

        var commands = ShellCommandRegistry.BuildPanelCommands(catalog, panel => navigated.Add(panel.Id));

        // Every panel in sidebar order, then Settings with its own chord. Runtime (needs a newer runtime) is listed, disabled and without a chord;
        // every other panel keeps the chord it always had.
        Assert.Equal(16, commands.Count);
        Assert.Equal(
            catalog.SidebarOrder.Select(p => "nav." + p.Id).Append("nav.settings").ToArray(),
            commands.Select(c => c.Id).ToArray());
        Assert.Equal(
            ChordTexts.Take(11).Append(null).Concat(ChordTexts.Skip(11).Take(3)).Append(ShellShortcuts.SettingsText).ToArray(),
            commands.Select(c => c.Shortcut).ToArray());
        Assert.All(commands, c =>
        {
            Assert.StartsWith("Go to ", c.Title, StringComparison.Ordinal);
            Assert.Equal(ShellCommandRegistry.PanelCategory, c.Category);
            Assert.Equal(c.Id != "nav.ai-runtime", c.IsEnabled);
        });

        commands[12].Run();
        commands[15].Run();
        Assert.Equal(new[] { "registries", "settings" }, navigated);
    }

    // ------------------------------------------------------------------ Ctrl+, opens Settings

    [Fact]
    public void Ctrl_comma_is_the_settings_chord_and_nothing_else_is()
    {
        Assert.Equal("Ctrl+,", ShellShortcuts.SettingsText);
        Assert.True(ShellShortcuts.IsSettingsChord(Key.OemComma, ModifierKeys.Control));

        Assert.False(ShellShortcuts.IsSettingsChord(Key.OemComma, ModifierKeys.None));
        Assert.False(ShellShortcuts.IsSettingsChord(Key.OemComma, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(ShellShortcuts.IsSettingsChord(Key.OemComma, ModifierKeys.Alt));
        Assert.False(ShellShortcuts.IsSettingsChord(Key.OemPeriod, ModifierKeys.Control));
        Assert.False(ShellShortcuts.IsSettingsChord(Key.K, ModifierKeys.Control));
    }

    [Fact]
    public void Ctrl_comma_collides_with_no_other_chord_in_the_shell()
    {
        var catalog = new PanelCatalog(_services);
        var model = ShortcutCatalog.Build(catalog);

        var all = model.Panels.Rows.Concat(model.Others.SelectMany(s => s.Rows)).Select(r => r.Keys).ToList();

        Assert.Single(all, ShellShortcuts.SettingsText);
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());

        // Not a panel number either: no digit is a comma.
        Assert.Null(ShellShortcuts.PanelIndexFor(Key.OemComma, ModifierKeys.Control));
        Assert.False(ShellShortcuts.IsToggleThemeChord(Key.OemComma, ModifierKeys.Control));

        // The overlay lists it, worded for what it opens, in the "anywhere" section.
        var row = Assert.Single(model.Others[0].Rows, r => r.Keys == ShellShortcuts.SettingsText);
        Assert.StartsWith("Open Settings", row.Description, StringComparison.Ordinal);
        Assert.Equal(new[] { "Ctrl", "," }, row.Parts);
    }
}
