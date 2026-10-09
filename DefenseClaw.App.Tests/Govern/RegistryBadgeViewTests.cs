using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Documents;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The registry badge on the Skills and MCPs tables, as the operator sees it (CUST-276): <c>registry:&lt;id&gt;</c> under the name of an item a registry
/// promoted, a click on it and the row menu's "Open in Registries" both sending the Registries panel to the entry, and an item no registry promoted
/// looking exactly as it did. The attribution is read from a synthetic config.yaml (the rules a registry sync writes) and the rows are the fixtures of
/// <c>Fixtures/CliPayloads</c>.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public sealed class RegistryBadgeViewTests
{
    private const string Config = """
        asset_policy:
          enabled: true
          skill:
            registry:
              - name: pdf-tools
                reason: registry:corp-skills
              - name: notes-helper
                reason: registry:enterprise-skills-catalog-prod
          mcp:
            registry:
              - name: docs-search
                reason: registry:corp-skills
              - name: sse-server
                reason: registry:team.catalog
        """;

    [Fact]
    public void A_skill_a_registry_promoted_shows_its_registry_badge_under_its_name_and_the_others_stay_one_line()
    {
        using var scene = Scene.Open("Skills", 1400, 900);

        UiThread.Run(() =>
        {
            var pdf = scene.RowOf("pdf-tools");
            var badge = scene.BadgeOf(pdf)!;
            Assert.Equal("registry:corp-skills", badge.Text);
            Assert.True(badge.IsVisible);

            // The chip says where it came from and what a click does; a screen reader gets the same in words.
            var chip = (Border)VisualTreeHelper.GetParent(badge);
            Assert.Contains("“corp-skills”", (string)chip.ToolTip, StringComparison.Ordinal);
            Assert.Contains("Click to open its entry in Registries", (string)chip.ToolTip, StringComparison.Ordinal);
            Assert.Equal("Promoted by registry corp-skills. Opens its entry in Registries.", AutomationProperties.GetName(badge));

            // A long id is cut as the TUI cuts it; the whole of it is in the tooltip.
            var notes = scene.BadgeOf(scene.RowOf("notes-helper"))!;
            Assert.Equal("registry:enterprise-skil...", notes.Text);
            Assert.Contains("“enterprise-skills-catalog-prod”", (string)((Border)VisualTreeHelper.GetParent(notes)).ToolTip, StringComparison.Ordinal);

            // Rows no registry promoted: no badge, and the one line they always were.
            foreach (var name in new[] { "risky-skill", "draft-skill", "paused-skill" })
            {
                var row = scene.RowOf(name);
                Assert.Null(scene.BadgeOf(row));
                Assert.InRange(row.ActualHeight, 20, 32);
            }

            // The badge is on a second line: the name keeps its place and the row is taller by that line, no more.
            Assert.True(pdf.ActualHeight > scene.RowOf("risky-skill").ActualHeight + 12, $"{pdf.ActualHeight:0} against {scene.RowOf("risky-skill").ActualHeight:0}");
            Assert.True(pdf.ActualHeight < 60);

            scene.Grid.SelectedIndex = 0;
            scene.Host.Relayout();
            RenderTo.Png(scene.Host, "govern-skills-registry-badge-1400x900");
        });
    }

    [Fact]
    public void An_mcp_server_a_registry_promoted_shows_the_badge_of_its_own_source()
    {
        using var scene = Scene.Open("Mcps", 1400, 900);

        UiThread.Run(() =>
        {
            Assert.Equal("registry:corp-skills", scene.BadgeOf(scene.RowOf("docs-search"))!.Text);
            Assert.Equal("registry:team.catalog", scene.BadgeOf(scene.RowOf("sse-server"))!.Text);
            Assert.Null(scene.BadgeOf(scene.RowOf("remote-tools")));

            scene.Grid.SelectedIndex = 0;
            scene.Host.Relayout();
            RenderTo.Png(scene.Host, "govern-mcps-registry-badge-1400x900");
        });
    }

    [Theory]
    [InlineData("Skills", "pdf-tools", "skill", "corp-skills")]
    [InlineData("Mcps", "sse-server", "mcp", "team.catalog")]
    public void Clicking_the_badge_sends_the_registries_panel_to_the_entry(string panel, string name, string kind, string source)
    {
        using var scene = Scene.Open(panel, 1400, 900);

        UiThread.Run(() =>
        {
            var row = scene.RowOf(name);
            var chip = (Border)VisualTreeHelper.GetParent(scene.BadgeOf(row)!);

            // The click is the row's own command with the verb the menu uses; running it is the navigation and nothing else.
            var click = Assert.Single(chip.InputBindings.OfType<MouseBinding>());
            Assert.Equal(MouseAction.LeftClick, click.MouseAction);
            Assert.Same(((GovernRow)row.Item).ActionCommand, click.Command);
            Assert.Equal("OpenInRegistries", click.CommandParameter);
            Assert.Equal(Cursors.Hand, chip.Cursor);

            click.Command.Execute(click.CommandParameter);

            Assert.Equal(new NavigationRequest("registries", new RegistryFocus(kind, name, source)), scene.Services.Navigation.Pending);
            Assert.Empty(scene.Services.Cli.Activity);
            Assert.False(scene.ViewModel.IsConfirmOpen);
        });
    }

    [Theory]
    [InlineData("Skills", "pdf-tools", "risky-skill")]
    [InlineData("Mcps", "docs-search", "remote-tools")]
    public void The_row_menu_offers_open_in_registries_only_for_an_item_a_registry_promoted(string panel, string promoted, string plain)
    {
        using var scene = Scene.Open(panel, 1400, 900);

        UiThread.Run(() =>
        {
            Assert.Contains("Open in Registries", scene.MenuHeaders(scene.RowOf(promoted)));
            Assert.DoesNotContain("Open in Registries", scene.MenuHeaders(scene.RowOf(plain)));

            // The item is the row's command, with a name and an icon like the rest, and it is a read: no review opens.
            var row = scene.RowOf(promoted);
            Assert.True(VisualTree.Descendants<DcRowMenuButton>(row).Single().OpenMenu());
            try
            {
                var item = ContextMenuService.GetContextMenu(row)!.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Open in Registries");
                Assert.NotNull(item.Icon);
                Assert.Same(((GovernRow)row.Item).ActionCommand, item.Command);
                Assert.Equal("OpenInRegistries", item.CommandParameter);
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(item)));

                // Enabled whatever the trust of the list: a navigation changes nothing.
                scene.ViewModel.Trust.MarkFailed("could not read the list");
                Assert.False(scene.ViewModel.IsDataTrusted);
                Assert.True(item.IsEnabled);
                item.Command.Execute(item.CommandParameter);
                Assert.NotNull(scene.Services.Navigation.Pending);
                Assert.False(scene.ViewModel.IsConfirmOpen);
            }
            finally
            {
                ContextMenuService.GetContextMenu(row)!.IsOpen = false;
            }
        });
    }

    [Fact]
    public void The_details_of_a_promoted_skill_name_its_registry()
    {
        using var scene = Scene.Open("Skills", 1400, 900);

        UiThread.Run(() =>
        {
            scene.Grid.SelectedItem = scene.RowOf("pdf-tools").Item;
            scene.Host.Relayout();

            var texts = VisualTree.Descendants<TextBlock>(scene.RowOf("pdf-tools")).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("Registry", texts);
            Assert.Contains(texts, t => t == "registry:corp-skills");
        });
    }

    [Theory]
    [InlineData("Skills", "notes-helper")]
    [InlineData("Mcps", "docs-search")]
    public void At_the_minimum_window_the_badge_stays_inside_its_column_and_the_name_keeps_its_line(string panel, string name)
    {
        using var scene = Scene.Open(panel, 940, 620);

        UiThread.Run(() =>
        {
            var row = scene.RowOf(name);
            var badge = scene.BadgeOf(row)!;
            var chip = (Border)VisualTreeHelper.GetParent(badge);
            var cell = VisualTree.Descendants<DataGridCell>(row).First(c => (string)c.Column.Header == "Name");

            var bounds = chip.TransformToAncestor(cell).TransformBounds(new Rect(0, 0, chip.ActualWidth, chip.ActualHeight));
            Assert.True(bounds.Left >= 0 && bounds.Right <= cell.ActualWidth + 0.5, $"the badge is at {bounds.Left:0}..{bounds.Right:0} in a {cell.ActualWidth:0} DIP cell");

            var title = VisualTree.Descendants<TextBlock>(cell).First(t => t.Text == ((GovernRow)row.Item).DisplayTitle);
            Assert.True(title.ActualWidth > 20);

            scene.Grid.SelectedIndex = 0;
            scene.Host.Relayout();
            RenderTo.Png(scene.Host, $"govern-{panel.ToLowerInvariant()}-registry-badge-940x620");
        });
    }

    [Theory]
    [InlineData("Default", "Dark", "default-dark")]
    [InlineData("Cisco", "Light", "cisco-light")]
    public void The_badge_is_drawn_from_the_looks_tokens_in_two_looks(string styleName, string modeName, string look)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(styleName), Enum.Parse<AppearanceMode>(modeName)));
        foreach (var (panel, name) in new[] { ("Skills", "pdf-tools"), ("Mcps", "docs-search") })
        {
            // One retry: under Cisco Light the first scene of a run has stalled without realizing a row on CI (see GovernTableTests).
            Scene OpenScene()
            {
                try
                {
                    return Scene.Open(panel, 1400, 900);
                }
                catch (TimeoutException)
                {
                    return Scene.Open(panel, 1400, 900);
                }
            }

            using var scene = OpenScene();
            UiThread.Run(() =>
            {
                var badge = scene.BadgeOf(scene.RowOf(name));
                Assert.NotNull(badge);
                Assert.True(badge!.IsVisible);
                Assert.True(badge.ActualWidth > 40);

                // The chip's fill and its text are the style's own tokens, not colours of this view.
                var chip = (Border)VisualTreeHelper.GetParent(badge);
                Assert.Same(Application.Current.FindResource("DcSubtleBrush"), chip.Background);
                Assert.NotNull(chip.GetValue(TextElement.ForegroundProperty));

                scene.Grid.SelectedIndex = 0;
                scene.Host.Relayout();
                RenderTo.Png(scene.Host, $"govern-{panel.ToLowerInvariant()}-registry-badge-{look}");
            });
        }
    }

    // ------------------------------------------------------------------ the scene

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private Scene(string panel, int width, int height)
        {
            Services = TestServices.Create(_temp, Config);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(Services, width, height);
                _ = panel == "Mcps" ? (FrameworkElement)shell.Show<McpsPanel>() : shell.Show<SkillsPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (GovernPanelViewModelBase)Shell.ViewModel);
            UiThread.WaitFor(() => ViewModel.State != GovernState.Loading, "first read finished");
            UiThread.Run(() =>
            {
                ViewModel.Trust.MarkComplete(); // the stand-in for a successful read: the row actions in these tests are meant to work
                foreach (var row in ViewModel.ParseRows(PayloadFixtures.Read(panel == "Mcps" ? "mcp-list.single-connector.json" : "skill-list.single-connector.json")))
                {
                    ViewModel.Rows.Add(row);
                }

                ViewModel.State = GovernState.Loaded;
                Host.Relayout();
            });
            UiThread.WaitFor(
                () =>
                {
                    Host.Relayout();
                    if (Grid is { ActualHeight: > 0, Items.Count: > 0 } grid && !VisualTree.Descendants<DataGridRow>(grid).Any())
                    {
                        grid.ScrollIntoView(grid.Items[0]);
                        grid.UpdateLayout();
                    }

                    return Grid is { ActualHeight: > 0 } && VisualTree.Descendants<DataGridRow>(Grid).Any();
                },
                "table laid out",
                timeoutMilliseconds: 60_000);
        }

        public AppServices Services { get; }

        public PanelShell Shell { get; }

        public GovernPanelViewModelBase ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public DataGrid Grid => (DataGrid)Shell.Page!.FindName("RowGrid");

        public static Scene Open(string panel, int width, int height) => new(panel, width, height);

        public DataGridRow RowOf(string name)
        {
            var row = VisualTree.Descendants<DataGridRow>(Grid).FirstOrDefault(r => r.Item is GovernRow g && g.Name == name);
            if (row is null)
            {
                Grid.ScrollIntoView(Grid.Items.OfType<GovernRow>().First(g => g.Name == name));
                Grid.UpdateLayout();
                row = VisualTree.Descendants<DataGridRow>(Grid).First(r => r.Item is GovernRow g && g.Name == name);
            }

            return row;
        }

        /// <summary>The badge's text block when the row shows one (a collapsed badge is not one), else null.</summary>
        public TextBlock? BadgeOf(DataGridRow row) =>
            VisualTree.Descendants<TextBlock>(row).FirstOrDefault(t => t.IsVisible && t.Text.StartsWith("registry:", StringComparison.Ordinal));

        /// <summary>The headers of the menu items the row's menu shows, opened the way the "…" button opens it.</summary>
        public List<string> MenuHeaders(DataGridRow row)
        {
            Assert.True(VisualTree.Descendants<DcRowMenuButton>(row).Single().OpenMenu());
            try
            {
                return ContextMenuService.GetContextMenu(row)!.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).Select(i => (string)i.Header).ToList();
            }
            finally
            {
                ContextMenuService.GetContextMenu(row)!.IsOpen = false;
            }
        }

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            Services.Dispose();
            _temp.Dispose();
        }
    }
}
