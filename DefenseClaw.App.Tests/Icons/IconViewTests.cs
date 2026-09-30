using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Shell;
using Wpf.Ui.Controls;

namespace DefenseClaw.App.Tests.Icons;

/// <summary>
/// Icons in the views (CUST-196): every panel names its glyph and section once and the sidebar, the page header and the
/// command palette agree with the catalog; a card header, the sidebar and the page header take their colour from the
/// style's tokens (and the card header follows a live switch); and the XAML names only sections, tints and symbols that exist.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class IconViewTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ------------------------------------------------------------------ panel -> section -> glyph

    [Fact]
    public void Every_panel_has_a_section_and_the_glyph_the_catalog_gives_it()
    {
        using var services = TestServices.Create(_temp);
        var catalog = new PanelCatalog(services);

        Assert.Equal(
            catalog.Panels.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal),
            DcSections.PanelIds.OrderBy(id => id, StringComparer.Ordinal));

        foreach (var panel in catalog.Panels)
        {
            var section = DcSections.OfPanelId(panel.Id);
            Assert.Contains(section, DcSections.All);
            Assert.Equal(section, DcSections.OfPanelView(panel.ViewType));
            Assert.Equal(panel.Icon, DcSections.IconOfPanelId(panel.Id));
        }
    }

    [Fact]
    public void The_sections_group_the_panels_the_way_the_operator_thinks_of_them()
    {
        Assert.Equal("Overview", DcSections.OfPanelId("overview"));
        Assert.All(new[] { "alerts", "audit", "logs", "activity" }, id => Assert.Equal("Observe", DcSections.OfPanelId(id)));
        Assert.All(new[] { "skills", "mcps", "plugins", "tools" }, id => Assert.Equal("Govern", DcSections.OfPanelId(id)));
        Assert.All(new[] { "inventory", "ai-discovery", "registries" }, id => Assert.Equal("Discover", DcSections.OfPanelId(id)));
        Assert.Equal("Setup", DcSections.OfPanelId("setup"));
        Assert.Null(DcSections.OfPanelId("no-such-panel"));
        Assert.Null(DcSections.OfPanelView(typeof(string)));
        Assert.Null(DcSections.OfPanelView(null));
    }

    [Fact]
    public void Each_page_header_shows_its_panels_glyph_in_its_panels_section()
    {
        // Read from the panel's XAML: the header is declared there, and building all thirteen views only to look at one
        // attribute of each would tie this test to whatever a view needs at construction. Overview keeps the DcPageHeader
        // block until it is recomposed; every other panel opens with the compact DcPageToolbar (CUST-208).
        using var services = TestServices.Create(_temp);
        var catalog = new PanelCatalog(services);
        var panels = Path.Combine(AppDirectory(), "Views", "Panels");

        foreach (var panel in catalog.Panels)
        {
            var text = File.ReadAllText(Path.Combine(panels, panel.ViewType.Name + ".xaml"));
            var header = Regex.Match(text, @"<(?:HeaderedContentControl\b[^>]*DcPageHeader|ctl:DcPageToolbar\b)[^>]*>");

            Assert.True(header.Success, $"{panel.Title} has neither a DcPageHeader nor a DcPageToolbar");
            Assert.Equal(panel.Icon.ToString(), Regex.Match(header.Value, @"DcIcon\.Symbol=""([^""]+)""").Groups[1].Value);
            Assert.Equal(DcSections.OfPanelId(panel.Id), Regex.Match(header.Value, @"DcIcon\.Section=""([^""]+)""").Groups[1].Value);
        }
    }

    [Fact]
    public void Every_command_the_palette_lists_has_a_glyph_of_its_own_in_a_known_section()
    {
        var ids = new List<string>
        {
            "app.refresh-panel", "app.refresh-gateway", "app.config-editor", "app.check-updates", "app.toggle-autostart",
            "app.shortcuts", "appearance.default", "appearance.linear", "appearance.tui", "appearance.toggle",
            "appearance.system", "gateway.start", "gateway.stop", "gateway.restart",
        };
        ids.AddRange(DcSections.PanelIds.Select(id => $"nav.{id}"));

        foreach (var id in ids)
        {
            var (icon, section) = DcSections.OfCommand(id);

            Assert.NotEqual(SymbolRegular.Circle24, icon);
            Assert.Contains(section, DcSections.All);
        }

        // A "Go to" row wears its panel's own glyph and section; an id nobody knows gets a neutral one, not a crash.
        Assert.Equal(SymbolRegular.PuzzlePiece24, DcSections.OfCommand("nav.skills").Icon);
        Assert.Equal("Govern", DcSections.OfCommand("nav.skills").Section);
        Assert.Equal(SymbolRegular.Circle24, DcSections.OfCommand("something.new").Icon);
        Assert.Equal(SymbolRegular.Circle24, DcSections.OfCommand(null).Icon);
    }

    [Fact]
    public void The_XAML_converters_answer_what_the_sections_table_does()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Equal("Govern", PanelSectionConverter.Instance.Convert(typeof(DefenseClaw.App.Views.Panels.SkillsPanel), typeof(string), null, culture));
        Assert.Null(PanelSectionConverter.Instance.Convert(null, typeof(string), null, culture));
        Assert.Null(PanelSectionConverter.Instance.Convert("not a type", typeof(string), null, culture));

        Assert.Equal(SymbolRegular.WrenchScrewdriver24, CommandIconConverter.Instance.Convert("nav.tools", typeof(SymbolRegular), null, culture));
        Assert.Equal("Updates", CommandSectionConverter.Instance.Convert("app.check-updates", typeof(string), null, culture));
        Assert.Equal("Overview", CommandSectionConverter.Instance.Convert("gateway.restart", typeof(string), null, culture));

        Assert.Throws<NotSupportedException>(() => PanelSectionConverter.Instance.ConvertBack("Govern", typeof(Type), null, culture));
        Assert.Throws<NotSupportedException>(() => CommandIconConverter.Instance.ConvertBack(SymbolRegular.Circle24, typeof(string), null, culture));
        Assert.Throws<NotSupportedException>(() => CommandSectionConverter.Instance.ConvertBack("Setup", typeof(string), null, culture));
    }

    // ------------------------------------------------------------------ card headers

    [Fact]
    public void A_card_header_tints_its_glyph_by_section_or_by_palette_and_follows_a_live_switch()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var bySection = new DcCardHeader { Symbol = SymbolRegular.Shield24, Section = "Govern", Content = "Scanners" };
            var byTint = new DcCardHeader { Symbol = SymbolRegular.Warning24, Section = "Govern", Tint = "Orange", Content = "Attention" };
            var untinted = new DcCardHeader { Symbol = SymbolRegular.Info24, Content = "Plain" };
            var titleOnly = new DcCardHeader { Content = "No glyph" };
            var window = new Window { Content = new StackPanel { UseLayoutRounding = false, Children = { bySection, byTint, untinted, titleOnly } } };
            try
            {
                foreach (var header in new[] { bySection, byTint, untinted, titleOnly })
                {
                    _ = header.ApplyTemplate();
                }

                Assert.Equal(AppearanceFixture.ColorOf("DcSectionGovernBrush"), GlyphColor(bySection));
                Assert.Equal(AppearanceFixture.ColorOf("DcTintOrangeBrush"), GlyphColor(byTint));
                Assert.Equal(AppearanceFixture.ColorOf("DcIconBrush"), GlyphColor(untinted));
                Assert.Equal(Visibility.Collapsed, Glyph(titleOnly).Visibility);

                fixture.Service.SetStyle(AppearanceStyle.Tui);
                Assert.Equal(AppearanceFixture.ColorOf("DcSectionGovernBrush"), GlyphColor(bySection));
                Assert.Equal(AppearanceFixture.ColorOf("DcAccentBrush"), GlyphColor(bySection));

                fixture.Service.SetStyle(AppearanceStyle.Default);
                Assert.Equal(AppearanceFixture.ColorOf("DcSectionGovernBrush"), GlyphColor(bySection));
                Assert.Equal(AppearanceFixture.ColorOf("DcTintOrangeBrush"), GlyphColor(byTint));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void A_card_header_is_read_once_as_its_title_and_keeps_its_heading_level()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            var header = new DcCardHeader { Symbol = SymbolRegular.PaintBrush24, Section = "Setup", Content = "Style" };
            AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);

            // On a real presentation source: the heading level is a binding to the header, which attaches once the title is in a tree.
            using var host = new OffscreenHost(header, 300, 100);

            // No peer of its own (the glyph is not announced as a group or a control) ...
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(header));

            // ... the title text carries the heading level, and the glyph is a decorative icon.
            var title = Visual(header).OfType<System.Windows.Controls.TextBlock>().Single(t => t.Text == "Style");
            Assert.Equal(AutomationHeadingLevel.Level2, AutomationProperties.GetHeadingLevel(title));
            Assert.IsType<DcSymbolIcon>(Glyph(header));
        });
    }

    // ------------------------------------------------------------------ the sidebar

    [Theory]
    [InlineData("Default")]
    [InlineData("Linear")]
    [InlineData("Tui")]
    public void The_sidebar_icons_wear_their_sections_tint_under_the_style_on_screen(string styleName)
    {
        var style = Enum.Parse<AppearanceStyle>(styleName);
        using var fixture = new AppearanceFixture(new AppearanceSettings(style, AppearanceMode.Dark));
        using var services = TestServices.Create(_temp);
        var catalog = new PanelCatalog(services);

        UiThread.Run(() =>
        {
            var navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Left, OpenPaneLength = 220 };

            // What MainWindow.xaml puts in the NavigationView's resources (the icon MainWindow.BuildNavigation makes is a stock SymbolIcon).
            navigation.Resources[typeof(SymbolIcon)] = new Style(typeof(SymbolIcon), (Style)Application.Current.FindResource("DcNavIcon"));

            var items = new List<(PanelDescriptor Panel, DcNavigationItem Item)>();
            foreach (var panel in catalog.Panels)
            {
                var item = new DcNavigationItem
                {
                    Content = panel.Title,
                    Icon = new SymbolIcon { Symbol = panel.Icon },
                    TargetPageType = panel.ViewType,
                    Height = 34,
                };
                _ = navigation.MenuItems.Add(item);
                items.Add((panel, item));
            }

            using var host = new OffscreenHost(navigation, 300, 700);

            foreach (var (panel, item) in items)
            {
                var icon = Visual(item).OfType<SymbolIcon>().First();
                var section = DcSections.OfPanelId(panel.Id)!;
                var expected = AppearanceFixture.ColorOf($"DcNavIcon{section}Brush");

                Assert.Equal(expected, ((SolidColorBrush)icon.Foreground).Color);
            }

            // Linear is coloured (each section its own tint); Default and TUI keep the sidebar in one quiet colour.
            var colours = items.Select(i => ((SolidColorBrush)Visual(i.Item).OfType<SymbolIcon>().First().Foreground).Color).Distinct().Count();
            Assert.Equal(style == AppearanceStyle.Linear ? 5 : 1, colours);
        });
    }

    // ------------------------------------------------------------------ what the XAML names

    [Fact]
    public void The_views_name_only_sections_and_tints_that_exist()
    {
        var unknown = new List<string>();
        var palette = new[] { "Indigo", "Blue", "Violet", "Teal", "Green", "Amber", "Orange", "Red", "Pink", "Gray" };

        foreach (var file in OwnedXaml())
        {
            var text = File.ReadAllText(file);

            // A value that is a binding (the palette's rows pick theirs per command) is checked by the converter's own test.
            foreach (Match m in Regex.Matches(text, @"\bSection=""([^""{]*)"""))
            {
                if (!DcSections.All.Contains(m.Groups[1].Value))
                {
                    unknown.Add($"{Path.GetFileName(file)}: Section=\"{m.Groups[1].Value}\"");
                }
            }

            foreach (Match m in Regex.Matches(text, @"\bTint=""([^""]*)"""))
            {
                if (!palette.Contains(m.Groups[1].Value))
                {
                    unknown.Add($"{Path.GetFileName(file)}: Tint=\"{m.Groups[1].Value}\"");
                }
            }
        }

        Assert.True(unknown.Count == 0, string.Join("; ", unknown));
    }

    [Fact]
    public void Every_icon_in_the_shell_and_the_panels_is_the_decorative_one()
    {
        // A stock SymbolIcon exposes its glyph as a Text element in UI Automation; DcSymbolIcon does not. The views this file
        // set covers use no other (WPF-UI's own <ui:SymbolIcon> is still what the wizard and the config editor use).
        var offenders = new List<string>();
        foreach (var file in OwnedXaml())
        {
            var text = File.ReadAllText(file);
            if (Regex.IsMatch(text, @"<ui:SymbolIcon[\s/>]") || text.Contains("{ui:SymbolIcon ", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(offenders.Count == 0, "Stock SymbolIcon in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_icon_sizes_are_16_in_a_row_20_in_a_header_and_48_muted_in_an_empty_state()
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(AppearanceStyle.Linear, AppearanceMode.Dark));

        UiThread.Run(() =>
        {
            SymbolIcon Icon(string style) => new DcSymbolIcon(SymbolRegular.Shield24) { Style = (Style)Application.Current.FindResource(style) };

            var window = new Window { Content = new StackPanel { UseLayoutRounding = false, Children = { Icon("DcIcon"), Icon("DcIconMuted"), Icon("DcIconCard"), Icon("DcIconNav"), Icon("DcEmptyIcon") } } };
            try
            {
                var icons = ((StackPanel)window.Content).Children.OfType<SymbolIcon>().ToArray();

                Assert.Equal(new[] { 16.0, 16.0, 20.0, 20.0, 48.0 }, icons.Select(i => i.FontSize).ToArray());
                Assert.Equal(AppearanceFixture.ColorOf("DcIconBrush"), ((SolidColorBrush)icons[0].Foreground).Color);
                Assert.Equal(AppearanceFixture.ColorOf("DcIconMutedBrush"), ((SolidColorBrush)icons[1].Foreground).Color);
                Assert.Equal(AppearanceFixture.ColorOf("DcIconMutedBrush"), ((SolidColorBrush)icons[4].Foreground).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------ helpers

    private static SymbolIcon Glyph(DcCardHeader header) => Visual(header).OfType<SymbolIcon>().First();

    private static Color GlyphColor(DcCardHeader header) => ((SolidColorBrush)Glyph(header).Foreground).Color;

    private static IEnumerable<DependencyObject> Visual(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Visual(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>The XAML of the shell and the panels (the files this design covers), found above the test output directory.</summary>
    private static IEnumerable<string> OwnedXaml()
    {
        var app = AppDirectory();
        var files = new List<string>
        {
            Path.Combine(app, "MainWindow.xaml"),
            Path.Combine(app, "Views", "TrayFlyoutWindow.xaml"),
        };
        foreach (var folder in new[] { Path.Combine("Views", "Panels"), Path.Combine("Views", "Shell"), Path.Combine("Views", "Updates") })
        {
            files.AddRange(Directory.EnumerateFiles(Path.Combine(app, folder), "*.xaml", SearchOption.AllDirectories));
        }

        return files;
    }

    private static string AppDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "DefenseClaw.App", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new FileNotFoundException("DefenseClaw.App\\MainWindow.xaml was not found above the test output directory.");
    }
}
