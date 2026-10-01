using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DefenseClaw.App.Services;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The Registries panel as a real view, over a synthetic cached <c>index.json</c> (the <c>registry-index.synced.json</c>
/// fixture: one clean entry, one with two MEDIUM findings, one with four HIGH ones, one whose fetch failed, one
/// pending). An entry with an error or findings gets a second line under its row — the error in the Bad tone, a
/// findings badge toned by the worst severity — and every other entry stays one compact line.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class RegistriesPanelLayoutTests
{
    [Fact]
    public void An_entry_with_an_error_or_findings_gets_a_detail_line_and_the_others_stay_one_line()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var rows = scene.Rows();
            Assert.Equal(new[] { "pdf-tools", "docs-mcp", "remote-mcp", "broken-skill", "new-entry" }, rows.Select(r => r.Name).ToArray());

            // Nothing extra for a clean entry or a pending one (the presenter keeps a 1 DIP rule, whatever it holds).
            Assert.All(new[] { "pdf-tools", "new-entry" }, name => Assert.True(scene.DetailHeight(name) <= 2, $"{name} drew a detail line of {scene.DetailHeight(name)}"));

            // A second line for the other three.
            Assert.All(new[] { "docs-mcp", "remote-mcp", "broken-skill" }, name => Assert.True(scene.DetailHeight(name) >= 18, $"{name} has no detail line"));

            // The words on screen: a count badge each for the two scanned entries, the error line for the failed one.
            Assert.Equal(new[] { "2 findings" }, scene.VisibleTexts("docs-mcp"));
            Assert.Equal(new[] { "4 findings" }, scene.VisibleTexts("remote-mcp"));
            Assert.Equal(new[] { "Error: fetch failed: HTTP 404" }, scene.VisibleTexts("broken-skill"));
            Assert.Empty(scene.VisibleTexts("pdf-tools"));

            scene.Render("registries-entries-1400x900");
        });
    }

    [Fact]
    public void The_badge_carries_the_severity_tone_the_tooltip_and_an_accessible_name_and_the_error_is_bad()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var docs = scene.Badge("docs-mcp");
            Assert.Equal("Medium", docs.Tag);
            Assert.Equal("2 findings, worst severity MEDIUM", System.Windows.Automation.AutomationProperties.GetName(docs));

            // The tooltip is a wrapping text block bound to the row's own FindingsToolTip (a tooltip's content is only
            // resolved once it opens); the same words are the badge's help text, which is bound in the row's context.
            var tip = Assert.IsType<TextBlock>(docs.ToolTip);
            Assert.Equal("FindingsToolTip", System.Windows.Data.BindingOperations.GetBinding(tip, TextBlock.TextProperty)!.Path.Path);
            Assert.Contains("worst severity MEDIUM", System.Windows.Automation.AutomationProperties.GetHelpText(docs), StringComparison.Ordinal);

            var remote = scene.Badge("remote-mcp");
            Assert.Equal("High", remote.Tag);
            Assert.Equal("4 findings, worst severity HIGH", System.Windows.Automation.AutomationProperties.GetName(remote));

            var error = scene.ErrorLine("broken-skill");
            Assert.Equal("Bad", error.Tag);
            Assert.Equal("error: fetch failed: HTTP 404", System.Windows.Automation.AutomationProperties.GetName(error));
        });
    }

    [Fact]
    public void At_the_narrowest_width_the_detail_lines_stay_inside_the_entries_grid()
    {
        // The minimum window width (the entries card is then at its 340 DIP floor), tall enough to see the entries.
        using var scene = Scene.Open(940, 1000);

        UiThread.Run(() =>
        {
            var grid = scene.EntriesGrid;
            Assert.All(new[] { "docs-mcp", "remote-mcp", "broken-skill" }, name =>
            {
                var line = scene.DetailHeight(name);
                Assert.True(line >= 18, $"{name} has no detail line at the narrowest width");
                Assert.True(scene.DetailRight(name) <= grid.ActualWidth + 1, $"{name}'s detail line runs past the grid");
            });

            scene.Render("registries-entries-940x1000");
        });
    }

    // ------------------------------------------------------------------ the minimum window

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void At_the_minimum_window_the_entries_grid_keeps_room_with_or_without_an_action_banner(bool withActionMessage)
    {
        // 940 x 620 DIPs leave the page ~711 x 538; the toolbar, the banner and the default-deny card used to take all of it
        // (the entries got 18 DIPs, and none at all under an action banner).
        using var scene = Scene.Open(940, 620, actionMessage: withActionMessage ? "Synced corp-skills: 5 entries, 1 clean." : null);

        UiThread.Run(() =>
        {
            var grid = scene.EntriesGrid;
            Assert.True(grid.ActualHeight >= 150, $"the entries grid is {grid.ActualHeight:0} DIPs tall");

            // The page scrolls, so what counts is what the operator sees once the block is scrolled to the top of the page.
            scene.ScrollBlockIntoView();
            var visible = scene.VisibleHeight(grid);
            Assert.True(visible >= 150, $"{visible:0} DIPs of the entries grid are on screen");

            // The sources grid has room too.
            Assert.True(scene.SourcesGrid.ActualHeight >= 150, $"the sources grid is {scene.SourcesGrid.ActualHeight:0} DIPs tall");

            RenderTo.Png(scene.Host, withActionMessage ? "registries-940x620-message" : "registries-940x620");
        });
    }

    [Theory]
    [InlineData(940, 620)]
    [InlineData(1400, 900)]
    public void The_sources_columns_that_matter_are_readable_at_every_window_size(int width, int height)
    {
        // Every column used to sit at 46 DIPs (the Location one at 20) below ~1,820 DIPs of window.
        using var scene = Scene.Open(width, height);

        UiThread.Run(() =>
        {
            var columns = scene.SourcesGrid.Columns.ToDictionary(c => (string)c.Header, c => c.ActualWidth);
            Assert.All(new[] { "Id", "Enabled", "Entries" }, header => Assert.True(columns[header] >= 80, $"{header} is {columns[header]:0} DIPs wide"));

            // Whatever is shown is shown at its floor or better; the rest waits past the edge, reached by scrolling.
            Assert.All(scene.SourcesGrid.Columns, c => Assert.True(c.ActualWidth >= c.MinWidth - 0.5, $"{c.Header} is {c.ActualWidth:0} DIPs wide"));

            // The three leading columns are all inside the grid's viewport, not past its right edge.
            var viewport = VisualTree.Find<ScrollViewer>(scene.SourcesGrid)!.ViewportWidth;
            Assert.True(columns["Id"] + columns["Enabled"] + columns["Entries"] <= viewport + 1, "the leading columns do not fit the grid");

            RenderTo.Png(scene.Host, $"registries-sources-{width}x{height}");
        });
    }

    [Fact]
    public void The_source_details_show_where_it_comes_from_and_the_raw_fields_wait_behind_an_expander()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            var texts = VisualTree.Descendants<TextBlock>(scene.Shell.Page!).Where(t => t.IsVisible).Select(t => t.Text).ToArray();
            Assert.Contains("https://registry.example.test/defenseclaw-registry.yaml", texts);
            Assert.Contains("2026-09-20", string.Join('\n', texts), StringComparison.Ordinal);

            // Raw fields: closed until asked for, so the entries keep the room.
            var raw = VisualTree.Find<Expander>(scene.Shell.Page!, e => System.Windows.Automation.AutomationProperties.GetName(e) == "Raw fields of the selected source")!;
            Assert.False(raw.IsExpanded);
            var closedHeight = scene.EntriesGrid.ActualHeight;

            raw.IsExpanded = true;
            scene.Host.Relayout();
            Assert.True(scene.EntriesGrid.ActualHeight >= 150, "opening the raw fields squeezed the entries");
            Assert.True(scene.EntriesGrid.ActualHeight >= closedHeight - 2, $"the entries went from {closedHeight:0} to {scene.EntriesGrid.ActualHeight:0} DIPs");
        });
    }

    [Fact]
    public void The_default_deny_card_is_closed_by_default_and_its_header_says_where_each_switch_stands()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            var card = VisualTree.Find<Wpf.Ui.Controls.CardExpander>(scene.Shell.Page!)!;
            Assert.False(card.IsExpanded);
            Assert.Equal(new[] { "Skills: optional", "MCP servers: optional" }, scene.HeaderChips(card));

            scene.ViewModel.SkillsRegistryRequired = true;
            scene.Host.Relayout();
            Assert.Equal(new[] { "Skills: required", "MCP servers: optional" }, scene.HeaderChips(card));

            scene.ViewModel.McpsRegistryRequired = true;
            scene.Host.Relayout();
            Assert.Equal(new[] { "Skills: required", "MCP servers: required" }, scene.HeaderChips(card));
        });
    }

    [Fact]
    public void Both_grids_still_virtualize_their_rows_inside_the_scrolling_page()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                scene.ViewModel.Entries.Add(new RegistryEntryRow { Name = $"entry-{i:000}", Type = "skill", Status = "clean" });
                scene.ViewModel.Sources.Add(new RegistrySourceRow { Id = $"source-{i:000}", Fields = Array.Empty<RegistryFieldRow>() });
            }

            scene.Host.Relayout();

            Assert.InRange(VisualTree.Descendants<DataGridRow>(scene.EntriesGrid).Count(), 1, 40);
            Assert.InRange(VisualTree.Descendants<DataGridRow>(scene.SourcesGrid).Count(), 1, 40);

            // Both are scrollers inside a scrolling page: a wheel notch a grid cannot use goes on to the page.
            Assert.True(NestedScroll.GetForwardWheel(scene.SourcesGrid));
            Assert.True(NestedScroll.GetForwardWheel(scene.EntriesGrid));
        });
    }

    [Fact]
    public void The_could_not_read_card_is_never_cut_off_and_its_button_can_be_reached()
    {
        // No CLI, no sources: the panel says the read failed, with a Try again button. In a short window the card used to
        // get less room than it needs (the row it sat in was smaller than it), which clipped the button to a sliver.
        using var scene = Scene.Open(940, 620, withSource: false);

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.ShowUnavailable);
            var button = VisualTree.Find<Wpf.Ui.Controls.Button>(scene.Shell.Page!, b => b.IsVisible && b.Content as string == "Try again")!;
            var stack = (FrameworkElement)VisualTreeHelper.GetParent(button);

            // A layout clip is what WPF sets on an element that was given less room than it asked for.
            Assert.Null(LayoutInformation.GetLayoutClip(stack));
            Assert.True(stack.ActualHeight + stack.Margin.Top + stack.Margin.Bottom >= stack.DesiredSize.Height - 0.5);

            // The page scrolls to it: at the end of the page the whole button is inside the viewport.
            var scroll = scene.PageScroll;
            scroll.ScrollToEnd();
            scene.Host.Relayout();
            var bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= scroll.ViewportHeight + 0.5, $"the button is at {bounds.Top:0}..{bounds.Bottom:0} of a {scroll.ViewportHeight:0} DIP viewport");

            RenderTo.Png(scene.Host, "registries-unavailable-940x620");
        });
    }

    // ------------------------------------------------------------------ the scene

    // ------------------------------------------------------------------ the dense tables

    [Fact]
    public void The_sources_table_shows_the_enabled_state_as_a_pill_and_each_row_has_a_menu_that_opens_the_review()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.Equal(
                new[] { "Id", "Enabled", "Entries", "Kind", "Content", "Last sync", "Location", "Actions" },
                scene.SourcesGrid.Columns.Select(c => (string)c.Header).ToArray());

            var row = VisualTree.Descendants<DataGridRow>(scene.SourcesGrid).First();
            var pill = VisualTree.Descendants<DefenseClaw.App.Views.Controls.DcStatePill>(row).Single();
            Assert.Equal("enabled", pill.Word);
            Assert.Equal("Ok", pill.Tone);

            var menu = ContextMenuService.GetContextMenu(row)!;
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal("Sync…", items[0].Header);
            Assert.Equal("Remove…", items[^1].Header);
            Assert.Same(Application.Current.FindResource("DcToneCriticalBrush"), items[^1].Foreground);
            Assert.All(items, i => Assert.NotNull(i.Icon));

            // Sync opens the shared review (the exact command, nothing run) for the row's source.
            Assert.False(scene.ViewModel.Review.IsOpen);
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(scene.ViewModel.Review.IsOpen);
        });
    }

    [Fact]
    public void The_entries_table_shows_status_and_review_as_marks_with_words_and_approve_opens_the_review()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var entries = scene.EntriesGrid;
            Assert.Equal(new[] { "Name", "Type", "Status", "Review", "Actions" }, entries.Columns.Select(c => (string)c.Header).ToArray());

            var rows = VisualTree.Descendants<DataGridRow>(entries).ToList();
            var labels = VisualTree.Descendants<DefenseClaw.App.Views.Controls.DcStatusLabel>(entries).Select(l => l.Text).ToList();
            Assert.Contains("clean", labels);
            Assert.Contains("Approved", labels);

            // A skill entry can be reviewed; the menu's Approve opens the review for it.
            var pdf = rows.First(r => r.Item is RegistryEntryRow { Name: "pdf-tools" });
            pdf.IsSelected = true;
            var menu = ContextMenuService.GetContextMenu(pdf)!;
            var approve = menu.Items.OfType<MenuItem>().First();
            Assert.Equal("Approve…", approve.Header);
            Assert.Same(Application.Current.FindResource("DcToneCriticalBrush"), menu.Items.OfType<MenuItem>().Last().Foreground);
            approve.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(scene.ViewModel.Review.IsOpen);
        });
    }

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(int width, int height, bool withSource, string? actionMessage)
        {
            var folder = Path.Combine(_temp.Path, "registries", "corp-skills");
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.json"), PayloadFixtures.Read("registry-index.synced.json"));

            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                _ = shell.Show<RegistriesPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (RegistriesPanelViewModel)Shell.ViewModel);

            // The isolated services have no CLI, so the panel's own read ends at "not found"; settle that, then put the
            // synthetic source in front of it (the entries come from the index.json above, through the real reader).
            UiThread.WaitFor(() => ViewModel.HasLoaded && !ViewModel.IsLoading, "registries read finished");
            if (!withSource)
            {
                UiThread.Run(() => Host.Relayout());
                return;
            }

            UiThread.Run(() =>
            {
                var source = new RegistrySourceRow
                {
                    Id = "corp-skills",
                    Kind = "http_yaml",
                    Content = "both",
                    Enabled = true,
                    EntriesSummary = "5 (1 clean, 1 warning, 1 blocked, 1 error)",
                    LastSync = "2026-09-20T15:04:05+00:00",
                    LastStatus = "ok",
                    Location = "https://registry.example.test/defenseclaw-registry.yaml",
                    Fields = new[] { new RegistryFieldRow("id", "corp-skills"), new RegistryFieldRow("kind", "http_yaml") },
                };
                ViewModel.CliErrorMessage = null;
                ViewModel.Sources.Add(source);
                ViewModel.HasSources = true;
                ViewModel.SelectedSource = source;
                if (actionMessage is not null)
                {
                    ViewModel.ActionMessage = actionMessage;
                }
            });
            UiThread.WaitFor(() => ViewModel.Entries.Count == 5 && !ViewModel.IsEntriesLoading, "entries read");
            UiThread.Run(() => Host.Relayout());
        }

        public PanelShell Shell { get; }

        public RegistriesPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public static Scene Open(int width, int height, bool withSource = true, string? actionMessage = null) =>
            new(width, height, withSource, actionMessage);

        public ScrollViewer PageScroll =>
            VisualTree.Find<ScrollViewer>(Shell.Page!, sv => System.Windows.Automation.AutomationProperties.GetName(sv) == "Registries page")
            ?? throw new InvalidOperationException("The page scroller was not built.");

        public Wpf.Ui.Controls.DataGrid SourcesGrid =>
            VisualTree.Find<Wpf.Ui.Controls.DataGrid>(
                Shell.Page!,
                g => System.Windows.Automation.AutomationProperties.GetName(g) == "Registry sources")
            ?? throw new InvalidOperationException("The sources grid was not built.");

        /// <summary>Scrolls the page so the sources + details block starts at the top of the viewport.</summary>
        public void ScrollBlockIntoView()
        {
            var top = SourcesGrid.TranslatePoint(new Point(0, 0), PageScroll).Y + PageScroll.VerticalOffset;
            PageScroll.ScrollToVerticalOffset(Math.Max(0, top - 12));
            Host.Relayout();
        }

        /// <summary>How many DIPs of the element are inside the page scroller's viewport right now.</summary>
        public double VisibleHeight(FrameworkElement element)
        {
            var bounds = element.TransformToAncestor(PageScroll).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return Math.Max(0, Math.Min(bounds.Bottom, PageScroll.ViewportHeight) - Math.Max(bounds.Top, 0));
        }

        /// <summary>The state chips in a default-deny expander's header: the visible "Skills: ..." and "MCP servers: ..." texts.</summary>
        public string[] HeaderChips(Wpf.Ui.Controls.CardExpander card) =>
            VisualTree.Descendants<TextBlock>(card)
                .Where(t => t.IsVisible && (t.Text.StartsWith("Skills:", StringComparison.Ordinal) || t.Text.StartsWith("MCP servers:", StringComparison.Ordinal)))
                .Select(t => t.Text)
                .ToArray();

        public Wpf.Ui.Controls.DataGrid EntriesGrid =>
            VisualTree.Find<Wpf.Ui.Controls.DataGrid>(
                Shell.Page!,
                g => System.Windows.Automation.AutomationProperties.GetName(g) == "Cached entries of the selected source")
            ?? throw new InvalidOperationException("The entries grid was not built.");

        public IReadOnlyList<RegistryEntryRow> Rows() => ViewModel.Entries.ToArray();

        private DataGridRow RowOf(string name) =>
            VisualTree.Descendants<DataGridRow>(EntriesGrid).First(r => r.Item is RegistryEntryRow { } e && e.Name == name);

        /// <summary>The height the row's details area takes (0 when the entry has nothing to say).</summary>
        public double DetailHeight(string name) => VisualTree.Find<DataGridDetailsPresenter>(RowOf(name))?.ActualHeight ?? 0;

        public double DetailRight(string name)
        {
            var presenter = VisualTree.Find<DataGridDetailsPresenter>(RowOf(name))!;
            return presenter.TranslatePoint(new Point(presenter.ActualWidth, 0), EntriesGrid).X;
        }

        /// <summary>The text of every visible text block inside the row's details area.</summary>
        public string[] VisibleTexts(string name)
        {
            var presenter = VisualTree.Find<DataGridDetailsPresenter>(RowOf(name));
            return presenter is null
                ? Array.Empty<string>()
                : VisualTree.Descendants<TextBlock>(presenter).Where(t => t.IsVisible && t.Text.Length > 0).Select(t => t.Text).ToArray();
        }

        public Border Badge(string name) =>
            VisualTree.Find<Border>(RowOf(name), b => b.Tag is string && b.IsVisible)
            ?? throw new InvalidOperationException($"{name} has no visible badge.");

        public TextBlock ErrorLine(string name) =>
            VisualTree.Find<TextBlock>(RowOf(name), t => t.IsVisible && t.Text.StartsWith("Error:", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{name} has no visible error line.");

        /// <summary>Scrolls the selected source's card so its entries are what the picture shows, then renders.</summary>
        public void Render(string fileName)
        {
            var scroll = VisualTree.Descendants<ScrollViewer>(Shell.Page!)
                .FirstOrDefault(sv => sv.IsAncestorOf(EntriesGrid) && sv.ScrollableHeight > 0);
            if (scroll is not null)
            {
                var top = EntriesGrid.TranslatePoint(new Point(0, 0), scroll).Y + scroll.VerticalOffset;
                scroll.ScrollToVerticalOffset(Math.Max(0, top - 70));
                Host.Relayout();
            }

            RenderTo.Png(Host, fileName);
        }

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
