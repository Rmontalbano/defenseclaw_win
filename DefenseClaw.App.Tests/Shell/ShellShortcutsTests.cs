using System.Windows.Input;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;

namespace DefenseClaw.App.Tests.Shell;

public sealed class ShellShortcutsTests : IDisposable
{
    /// <summary>
    /// The sidebar, top to bottom: the Mac's (v1.1.34) since CUST-383, with Policies and Sandboxes in Govern and the Windows-only Agents last in
    /// Discover. "ai-runtime" (CUST-309) and "agents" (CUST-355) need a newer runtime. The chords do not follow this order (see ChordIds).
    /// </summary>
    private static readonly string[] SidebarIds =
    {
        "overview", "alerts", "logs", "audit", "activity",
        "skills", "mcps", "plugins", "tools", "policies", "sandboxes",
        "inventory", "ai-discovery", "ai-runtime", "registries", "agents",
        "setup",
    };

    /// <summary>
    /// Every chord and the panel it opened before CUST-383 moved Policies and added Sandboxes: Ctrl+1 to Ctrl+0, then Ctrl+Shift+1 to Ctrl+Shift+5. Pinned
    /// here as data, not derived, so a change of the sidebar can never quietly re-bind one.
    /// </summary>
    private static readonly string[] ChordIds =
    {
        "overview", "alerts", "logs", "audit", "activity", "skills", "mcps", "plugins", "tools", "inventory",
        "ai-discovery", "registries", "setup", "policies", "ai-runtime",
    };

    /// <summary>The panels every runtime has, in chord order: the ones whose chord works whatever the connected runtime can do.</summary>
    private static readonly string[] BaseIds = ChordIds.Where(id => id is not "ai-runtime").ToArray();

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
    public void Ctrl_and_a_digit_selects_the_panel_at_that_place_in_the_chord_order(int digit, int expectedIndex)
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
    public void All_seventeen_panels_are_in_the_sidebar_in_the_macs_order_and_the_chords_keep_their_pinned_order()
    {
        var catalog = new PanelCatalog(_services);

        Assert.Equal(SidebarIds, catalog.SidebarOrder.Select(p => p.Id).ToArray());

        // Seventeen in the sidebar, two past the fifteen number chords: Agents (CUST-355) and Sandboxes (CUST-383) are reached from the sidebar and the palette.
        Assert.Equal(ShellShortcuts.NumberedPanels + 2, catalog.SidebarOrder.Count);

        // The chord order is pinned (PanelCatalog.ChordIds), then the panels with no chord: moving Policies and adding Sandboxes moved no chord.
        Assert.Equal(ChordIds, PanelCatalog.ChordIds.ToArray());
        Assert.Equal(ChordIds.Append("sandboxes").Append("agents").ToArray(), catalog.ChordOrder.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "ai-runtime", "agents" }, catalog.Panels.Where(p => p.Requires is not null).Select(p => p.Id).ToArray());
        Assert.Null(catalog.ChordTextOf(catalog.ById("agents")!));
        Assert.Null(catalog.ChordTextOf(catalog.ById("sandboxes")!));
    }

    [Fact]
    public async Task Every_chord_opens_the_panel_it_opened_before_the_sidebar_took_the_macs_order()
    {
        // A runtime that offers every panel (the 1.0 fixtures), so even the chord of a panel that needs one is live.
        using var temp = new TempDirectory();
        using var services = TestServices.Create(temp, runtimeProbeRunner: RuntimeFixtureRunner.For("95159fd"));
        _ = await services.Runtime.RefreshAsync();
        var catalog = new PanelCatalog(services);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ctrl+1"] = "overview", ["Ctrl+2"] = "alerts", ["Ctrl+3"] = "logs", ["Ctrl+4"] = "audit", ["Ctrl+5"] = "activity",
            ["Ctrl+6"] = "skills", ["Ctrl+7"] = "mcps", ["Ctrl+8"] = "plugins", ["Ctrl+9"] = "tools", ["Ctrl+0"] = "inventory",
            ["Ctrl+Shift+1"] = "ai-discovery", ["Ctrl+Shift+2"] = "registries", ["Ctrl+Shift+3"] = "setup", ["Ctrl+Shift+4"] = "policies",
            ["Ctrl+Shift+5"] = "ai-runtime",
        };

        foreach (var (chord, id) in expected)
        {
            // The key press, as the window's handler reads it, lands on the panel ...
            var parts = chord.Split('+');
            var modifiers = parts.Contains("Shift") ? ModifierKeys.Control | ModifierKeys.Shift : ModifierKeys.Control;
            var index = ShellShortcuts.PanelIndexFor(DigitKey(int.Parse(parts[^1], System.Globalization.CultureInfo.InvariantCulture)), modifiers);
            Assert.Equal(id, catalog.PanelForChord(index!.Value)?.Id);

            // ... and the panel's sidebar tooltip, palette row and the shortcuts list say that chord.
            var panel = catalog.ById(id)!;
            Assert.Equal(chord, catalog.ChordTextOf(panel));
            Assert.Equal(chord, catalog.ChordFor(panel));
        }

        // The panels without a chord stay without one, and no chord reaches them.
        foreach (var id in new[] { "sandboxes", "agents", "settings" })
        {
            Assert.Null(catalog.ChordFor(catalog.ById(id)!));
        }

        var palette = ShellCommandRegistry.BuildPanelCommands(catalog, _ => { });
        foreach (var (chord, id) in expected)
        {
            Assert.Equal(chord, palette.Single(c => c.Id == "nav." + id).Shortcut);
        }

        Assert.Equal(expected.Keys.ToArray(), ShortcutCatalog.Build(catalog).Panels.Rows.Select(r => r.Keys).ToArray());
        Assert.Equal(expected.Values.Select(id => catalog.ById(id)!.Title).ToArray(), ShortcutCatalog.Build(catalog).Panels.Rows.Select(r => r.Description).ToArray());
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
            new[]
            {
                "Monitor", "Monitor", "Monitor", "Monitor", "Monitor",
                "Govern", "Govern", "Govern", "Govern", "Govern", "Govern",
                "Discover", "Discover", "Discover", "Discover", "Discover",
                "Configure",
            },
            catalog.SidebarOrder.Select(p => p.Group).ToArray());
        Assert.Equal(18, catalog.Panels.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("overview", catalog.Default.Id);

        // The sidebar's labels are the Mac's (Runtime is the Mac's "Runtime" too), plus the Windows-only Agents.
        Assert.Equal(
            new[] { "Overview", "Alerts", "Logs", "Audit", "Activity", "Skills", "MCPs", "Plugins", "Tools", "Policies", "Sandboxes", "Inventory", "AI Discovery", "Runtime", "Registries", "Agents", "Setup" },
            catalog.SidebarOrder.Select(p => p.Title).ToArray());

        // Settings is the eighteenth: pinned below the groups, in no group, and outside the numbered order (Ctrl+, reaches it).
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
        Assert.Equal(BaseIds.Select(id => catalog.ById(id)!.Title).ToArray(), model.Panels.Rows.Select(r => r.Description).ToArray());
        Assert.DoesNotContain(model.Panels.Rows, r => r.Description is "Sandboxes" or "Agents");
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

        // Every panel in sidebar order (the Mac's since CUST-383), then Settings with its own chord. Runtime and Agents (need a newer runtime) are listed,
        // disabled and without a chord; Sandboxes is enabled (its page explains) with no chord; every other panel keeps the chord it always had.
        Assert.Equal(18, commands.Count);
        Assert.Equal(
            SidebarIds.Select(id => "nav." + id).Append("nav.settings").ToArray(),
            commands.Select(c => c.Id).ToArray());
        Assert.Equal(
            new[]
            {
                "Ctrl+1", "Ctrl+2", "Ctrl+3", "Ctrl+4", "Ctrl+5",
                "Ctrl+6", "Ctrl+7", "Ctrl+8", "Ctrl+9", "Ctrl+Shift+4", null,
                "Ctrl+0", "Ctrl+Shift+1", null, "Ctrl+Shift+2", null,
                "Ctrl+Shift+3",
                ShellShortcuts.SettingsText,
            },
            commands.Select(c => c.Shortcut).ToArray());
        Assert.All(commands, c =>
        {
            Assert.StartsWith("Go to ", c.Title, StringComparison.Ordinal);
            Assert.Equal(ShellCommandRegistry.PanelCategory, c.Category);
            Assert.Equal(c.Id is not ("nav.ai-runtime" or "nav.agents"), c.IsEnabled);
        });
        Assert.Equal("Govern · opens the Policies panel", commands.Single(c => c.Id == "nav.policies").Description);

        commands[14].Run();
        commands[10].Run();
        commands[17].Run();
        Assert.Equal(new[] { "registries", "sandboxes", "settings" }, navigated);
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
