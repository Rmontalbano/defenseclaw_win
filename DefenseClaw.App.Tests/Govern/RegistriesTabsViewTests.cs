using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DefenseClaw.App.Services;
using DefenseClaw.App.Services.Appearance;
using DefenseClaw.App.Tests.Appearance;
using DefenseClaw.App.Tests.TestSupport;
using DefenseClaw.App.ViewModels;
using DefenseClaw.App.Views.Controls;
using DefenseClaw.App.Views.Panels;

namespace DefenseClaw.App.Tests.Govern;

/// <summary>
/// The Registries panel's Sources / Entries / Approved tabs as a real view (CUST-276): the tab strip and its counts, the table of every source's
/// entries and the approved ones, the strip over a table narrowed by "Open in Registries", the source details (publisher, fetched at, the four counts)
/// and the layout at the window's minimum. The two synthetic sources are the ones of <see cref="RegistriesEntriesTabsTests"/>.
/// <para>
/// A PNG of each state is written when the <c>DC_RENDER_DIR</c> environment variable names a folder, and never otherwise.
/// </para>
/// </summary>
[Collection(UiCollection.Name)]
public class RegistriesTabsViewTests
{
    // ------------------------------------------------------------------ the tab strip

    [Fact]
    public void The_tab_strip_names_the_three_views_and_counts_the_rows_under_two_of_them()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var strip = scene.TabStrip;
            var segments = strip.Items.OfType<DcSegment>().ToList();
            Assert.Equal(new[] { "Sources", "Entries", "Approved" }, segments.Select(s => (string)s.Content).ToArray());
            Assert.Equal(new[] { "Sources", "Entries (9)", "Approved (3)" }, segments.Select(AutomationProperties.GetName).ToArray());
            Assert.Equal("sources", strip.SelectedValue);

            // Two-way: the strip drives the view-model and the view-model drives the strip.
            strip.SelectedValue = "approved";
            Assert.Equal(RegistriesPanelViewModel.ApprovedTab, scene.ViewModel.ActiveTab);
            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.EntriesTab;
            Assert.Equal("entries", strip.SelectedValue);
        });
    }

    [Fact]
    public void The_tab_strip_is_not_shown_when_there_are_no_sources()
    {
        using var scene = Scene.Open(1400, 900, withSources: false);

        UiThread.Run(() =>
        {
            Assert.False(scene.TabStrip.IsVisible);
            Assert.False(scene.Block("MasterDetail").IsVisible);
            Assert.False(scene.Block("EntriesBlock").IsVisible);
        });
    }

    // ------------------------------------------------------------------ the Entries tab

    [Fact]
    public void The_entries_tab_is_one_table_of_every_sources_entries_in_place_of_the_sources_block()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.EntriesTab;
            scene.Host.Relayout();

            Assert.False(scene.Block("MasterDetail").IsVisible);
            Assert.True(scene.Block("EntriesBlock").IsVisible);

            var grid = scene.TabGrid;
            Assert.Equal(
                new[] { "Source", "Name", "Type", "Status", "Severity", "Review", "Location", "Actions" },
                grid.Columns.Select(c => (string)c.Header).ToArray());
            Assert.Equal(9, grid.Items.Count);

            var rows = VisualTree.Descendants<DataGridRow>(grid).Select(r => r.Item).OfType<RegistryEntryRow>().ToList();
            Assert.Contains(rows, r => r is { SourceId: "corp-skills", Name: "pdf-tools" });
            Assert.Contains(rows, r => r is { SourceId: "team.catalog", Name: "wiki-skill" });

            // The words on screen: the source of a row, its status and review as marks with words.
            var labels = VisualTree.Descendants<DcStatusLabel>(grid).Select(l => l.Text).ToList();
            Assert.Contains("clean", labels);
            Assert.Contains("Approved", labels);
            Assert.Contains("Rejected", labels);

            // Nothing to scroll sideways to at this width, and the table has room.
            Assert.True(VisualTree.Find<ScrollViewer>(grid)!.ScrollableWidth < 1);
            Assert.True(grid.ActualHeight >= 250, $"the table is {grid.ActualHeight:0} DIPs tall");

            RenderTo.Png(scene.Host, "registries-entries-tab-1400x900");
        });
    }

    [Fact]
    public void The_toolbars_source_actions_are_for_the_sources_tab_and_sync_is_for_all_of_them()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.Equal(
                new[] { "Add source…", "Sync selected…", "Sync all…", "Disable…", "Remove…", "Refresh" },
                scene.VisibleToolbarActions().ToArray());

            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.EntriesTab;
            scene.Host.Relayout();
            Assert.Equal(new[] { "Sync selected…", "Sync all…", "Refresh" }, scene.VisibleToolbarActions().ToArray());

            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
            scene.Host.Relayout();
            Assert.Equal(new[] { "Sync selected…", "Sync all…", "Refresh" }, scene.VisibleToolbarActions().ToArray());
        });
    }

    [Fact]
    public void A_rows_menu_approves_or_rejects_that_entry_of_its_own_source_and_shows_its_source()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.EntriesTab;
            scene.Host.Relayout();

            var row = VisualTree.Descendants<DataGridRow>(scene.TabGrid).First(r => r.Item is RegistryEntryRow { SourceId: "team.catalog", Name: "pdf-tools" });
            row.IsSelected = true;
            var menu = ContextMenuService.GetContextMenu(row)!;
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Approve…", "Reject…", "Show source" }, items.Select(i => (string)i.Header).ToArray());
            Assert.All(items, i => Assert.NotNull(i.Icon));
            Assert.Same(Application.Current.FindResource("DcToneCriticalBrush"), items[1].Foreground);

            // Approve opens the shared review with the command for that row's own source.
            Assert.False(scene.ViewModel.Review.IsOpen);
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(scene.ViewModel.Review.IsOpen);
            Assert.Equal(
                new[] { "registry", "approve", "team.catalog", "pdf-tools", "--type", "skill", "--json" },
                scene.ViewModel.Review.CommandReview!.Steps[0].Argv);
            scene.ViewModel.Review.DismissCommand.Execute(null);

            // Show source goes to the Sources tab with that source chosen.
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(scene.ViewModel.IsSourcesTab);
            Assert.Equal("team.catalog", scene.ViewModel.SelectedSource!.Id);
        });
    }

    [Fact]
    public void The_action_bar_under_the_table_follows_the_selected_row_and_the_trust_of_the_list()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.EntriesTab;
            scene.Host.Relayout();

            var approve = scene.ActionButton("Approve…");
            var reject = scene.ActionButton("Reject…");
            var source = scene.ActionButton("Show source");
            Assert.False(approve.IsEnabled);
            Assert.False(reject.IsEnabled);
            Assert.False(source.IsEnabled);

            scene.ViewModel.SelectedTabEntry = scene.ViewModel.TabEntries.First(e => e is { SourceId: "team.catalog", Name: "docs-mcp" });
            Assert.True(approve.IsEnabled);
            Assert.True(reject.IsEnabled);
            Assert.True(source.IsEnabled);

            // A list that may not authorize a change turns the two reviews off and leaves the navigation.
            scene.ViewModel.Trust.MarkFailed("could not read the sources");
            scene.ViewModel.NotifyTrust();
            Assert.False(approve.IsEnabled);
            Assert.False(reject.IsEnabled);
            Assert.True(source.IsEnabled);
        });
    }

    // ------------------------------------------------------------------ the Approved tab

    [Fact]
    public void The_approved_tab_lists_the_approved_entries_of_every_source()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
            scene.Host.Relayout();

            var grid = scene.TabGrid;
            Assert.True(scene.Block("EntriesBlock").IsVisible);
            Assert.Equal(3, grid.Items.Count);
            Assert.All(grid.Items.OfType<RegistryEntryRow>(), row => Assert.True(row.Approved));
            Assert.Equal(
                new[] { "corp-skills/pdf-tools", "team.catalog/docs-mcp", "team.catalog/wiki-skill" },
                grid.Items.OfType<RegistryEntryRow>().Select(r => r.SourceId + "/" + r.Name).ToArray());
            Assert.False(scene.Empty.IsVisible);

            RenderTo.Png(scene.Host, "registries-approved-tab-1400x900");
        });
    }

    [Fact]
    public void An_approved_tab_with_nothing_approved_says_what_to_do_in_place_of_the_table()
    {
        using var scene = Scene.Open(1400, 900, secondSource: false, firstIndex: "registry-index.bare-array.json");

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.ApprovedTab;
            scene.Host.Relayout();

            Assert.False(scene.TabGrid.IsVisible);
            Assert.True(scene.Empty.IsVisible);
            Assert.Contains(
                VisualTree.Descendants<TextBlock>(scene.Empty),
                t => t.Text.StartsWith("No entries approved yet.", StringComparison.Ordinal));

            scene.ViewModel.ActiveTab = RegistriesPanelViewModel.EntriesTab;
            scene.Host.Relayout();
            Assert.True(scene.TabGrid.IsVisible);
            Assert.False(scene.Empty.IsVisible);
        });
    }

    // ------------------------------------------------------------------ Open in Registries, as the operator sees it

    [Fact]
    public void A_link_narrows_the_table_shows_the_strip_selects_the_row_and_the_strips_button_ends_it()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.Accept(new RegistryFocus("skill", "pdf-tools", "team.catalog"));
            scene.Host.Relayout();

            Assert.True(scene.ViewModel.IsEntriesTab);
            Assert.True(scene.Block("FocusStrip").IsVisible);
            Assert.Contains(
                VisualTree.Descendants<TextBlock>(scene.Block("FocusStrip")),
                t => t.Text == "Showing skill “pdf-tools” in every source (2 matches)");
            Assert.Equal(2, scene.TabGrid.Items.Count);

            // The entry of the source the item came from is the selected row, in sight, and takes the focus.
            var selected = Assert.IsType<RegistryEntryRow>(scene.TabGrid.SelectedItem);
            Assert.Equal(("team.catalog", "pdf-tools"), (selected.SourceId, selected.Name));
            var row = VisualTree.Descendants<DataGridRow>(scene.TabGrid).Single(r => ReferenceEquals(r.Item, selected));
            Assert.True(row.IsSelected);
            Assert.True(scene.View.FocusSelectedTabEntry());

            RenderTo.Png(scene.Host, "registries-entry-focus-1400x900");

            // Show all entries: the strip goes, the table is whole, the row stays selected.
            var button = VisualTree.Find<Wpf.Ui.Controls.Button>(scene.Block("FocusStrip"), b => b.Content as string == "Show all entries")!;
            button.Command.Execute(null);
            scene.Host.Relayout();
            Assert.False(scene.Block("FocusStrip").IsVisible);
            Assert.Equal(9, scene.TabGrid.Items.Count);
            Assert.Equal("pdf-tools", ((RegistryEntryRow)scene.TabGrid.SelectedItem).Name);
        });
    }

    [Fact]
    public void A_link_to_an_entry_that_is_not_cached_says_so_in_the_banner_and_leaves_the_table_whole()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            scene.ViewModel.Accept(new RegistryFocus("skill", "gone-skill", "corp-skills"));
            scene.Host.Relayout();

            var banner = VisualTree.Find<Wpf.Ui.Controls.InfoBar>(scene.View, b => b.Title == "That entry is not in the registries")!;
            Assert.True(banner.IsOpen);
            Assert.Contains("gone-skill", banner.Message, StringComparison.Ordinal);
            Assert.False(scene.Block("FocusStrip").IsVisible);
            Assert.Equal(9, scene.TabGrid.Items.Count);

            RenderTo.Png(scene.Host, "registries-entry-not-found-1400x900");
        });
    }

    [Fact]
    public void The_skills_row_action_reaches_the_registries_panel_through_the_catalog_and_lands_on_the_entry()
    {
        // The whole path: a promoted skill's "Open in Registries" -> the inbox -> the catalog hands it to the Registries panel when that comes on
        // screen -> the panel, whose first read is still open at that moment, selects the entry of the source the skill came from.
        using var temp = new TempDirectory();
        foreach (var (id, fixture) in new[] { ("corp-skills", "registry-index.synced.json"), ("team.catalog", "registry-index.second-source.json") })
        {
            var folder = Path.Combine(temp.Path, "registries", id);
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.json"), PayloadFixtures.Read(fixture));
        }

        using var services = TestServices.Create(
            temp,
            "asset_policy:\n  skill:\n    registry:\n      - name: pdf-tools\n        reason: registry:team.catalog\n");
        var gate = new TaskCompletionSource();
        OffscreenHost? host = null;
        try
        {
            RegistriesPanel view = null!;
            RegistriesPanelViewModel vm = null!;
            UiThread.Run(() =>
            {
                var skill = new SkillsPanelViewModel(services).ParseRows(PayloadFixtures.Read("skill-list.single-connector.json")).Single(r => r.Name == "pdf-tools");
                skill.ActionCommand.Execute("OpenInRegistries");
                Assert.Equal("registries", services.Navigation.Pending!.PanelId);

                var catalog = new PanelCatalog(
                    services,
                    new[]
                    {
                        new PanelDescriptor(
                            "registries",
                            "Registries",
                            "Discover",
                            Wpf.Ui.Controls.SymbolRegular.Library24,
                            typeof(RegistriesPanel),
                            s => new RegistriesPanelViewModel(s)
                            {
                                ReadSources = async (_, _) =>
                                {
                                    await gate.Task;
                                    var listing = InvocationFactory.Create(retainFullOutput: true, "registry", "list", "--json");
                                    InvocationFactory.Append(listing, PayloadFixtures.Read("registry-list.sources.json"));
                                    InvocationFactory.Finish(listing, 0);
                                    return listing;
                                },
                            }),
                    });

                view = (RegistriesPanel)catalog.GetPage(typeof(RegistriesPanel))!;
                host = new OffscreenHost(view, 1400, 900);
                vm = (RegistriesPanelViewModel)view.DataContext;

                // Coming on screen took the request; the panel is on its Entries tab and waiting for its read.
                Assert.Null(services.Navigation.Pending);
                Assert.True(vm.IsEntriesTab);
                Assert.False(vm.HasEntryFocus);
            });

            gate.SetResult();
            UiThread.WaitFor(() => vm.HasEntryFocus, "the link applied when the first read ended");

            UiThread.Run(() =>
            {
                host!.Relayout();
                var selected = Assert.IsType<RegistryEntryRow>(vm.SelectedTabEntry);
                Assert.Equal(("team.catalog", "pdf-tools"), (selected.SourceId, selected.Name));
                var grid = VisualTree.Find<Wpf.Ui.Controls.DataGrid>(view, g => AutomationProperties.GetName(g) == "Registry entries")!;
                Assert.Same(selected, grid.SelectedItem);
                Assert.Equal(2, grid.Items.Count);
                Assert.True(((FrameworkElement)view.FindName("FocusStrip")).IsVisible);
            });
        }
        finally
        {
            UiThread.Run(() => host?.Dispose());
        }
    }

    [Fact]
    public void The_focus_landing_scrolls_a_row_far_down_a_long_table_into_view_once_the_narrowing_ends()
    {
        using var scene = Scene.Open(1400, 900, extraEntries: 400);

        UiThread.Run(() =>
        {
            scene.ViewModel.Accept(new RegistryFocus("skill", "bulk-390", "team.catalog"));
            scene.Host.Relayout();
            Assert.Single(scene.TabGrid.Items);

            scene.ViewModel.ClearEntryFocusCommand.Execute(null);
            scene.Host.Relayout();
            UiThread.Settle();
            scene.Host.Relayout();

            // 409 rows, and the selected one is among the few that are realized.
            var realized = VisualTree.Descendants<DataGridRow>(scene.TabGrid).ToList();
            Assert.InRange(realized.Count, 1, 60);
            Assert.Contains(realized, r => r.Item is RegistryEntryRow { Name: "bulk-390" });
            Assert.True(scene.View.FocusSelectedTabEntry());
        });
    }

    // ------------------------------------------------------------------ the source details

    [Fact]
    public void The_source_details_show_the_publisher_when_it_was_fetched_and_the_four_counts()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            var texts = VisualTree.Descendants<TextBlock>(scene.Block("MasterDetail")).Where(t => t.IsVisible).Select(t => t.Text).ToList();
            Assert.Contains("Publisher", texts);
            Assert.Contains("Fetched at", texts);
            Assert.Equal("Example Corp", scene.Text("PublisherValue"));
            Assert.Equal(RegistrySourceRow.FormatTimestamp("2026-09-20T15:04:05.123456+00:00"), scene.Text("FetchedAtValue"));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", scene.Text("FetchedAtValue"));

            // The chips, in order, each toned by what it means.
            var chips = VisualTree.Descendants<Border>(scene.CountChips)
                .Select(chip => (Text: VisualTree.Find<TextBlock>(chip)?.Text, Tone: chip.Tag as string))
                .Where(c => c.Text is not null)
                .ToList();
            Assert.Equal(
                new (string?, string?)[] { ("5 entries", null), ("1 clean", "Ok"), ("1 warning", "Warn"), ("1 blocked", "Bad"), ("1 error", "Bad") },
                chips);

            RenderTo.Png(scene.Host, "registries-source-detail-1400x900");
        });
    }

    [Fact]
    public void A_source_that_was_never_synced_shows_dashes_and_no_counts()
    {
        using var scene = Scene.Open(1400, 900, secondSource: false);

        UiThread.Run(() =>
        {
            scene.ViewModel.SelectedSource = scene.ViewModel.Sources.Single(s => s.Id == "team.catalog");
        });
        UiThread.WaitFor(() => !scene.ViewModel.IsEntriesLoading, "the empty cache read");

        UiThread.Run(() =>
        {
            scene.Host.Relayout();
            Assert.False(scene.CountChips.IsVisible);
            Assert.Equal("—", scene.Text("PublisherValue"));
            Assert.Equal("—", scene.Text("FetchedAtValue"));
        });
    }

    [Fact]
    public void Choosing_another_source_clears_the_last_ones_facts_until_its_own_are_read()
    {
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            Assert.True(scene.ViewModel.SourceFacts.HasCounts);
            Assert.Equal("Example Corp", scene.ViewModel.SourceFacts.Publisher);

            // On the UI thread the read cannot land in the middle of this block: the facts are the dashes of "not read yet", not corp-skills'.
            scene.ViewModel.SelectedSource = scene.ViewModel.Sources.Single(s => s.Id == "team.catalog");
            Assert.False(scene.ViewModel.SourceFacts.HasCounts);
            Assert.Equal("—", scene.ViewModel.SourceFacts.Publisher);
        });
        UiThread.WaitFor(() => scene.ViewModel.SourceFacts.HasCounts, "team.catalog's index read");
        UiThread.Run(() => Assert.Equal("Team Catalog", scene.ViewModel.SourceFacts.Publisher));
    }

    // ------------------------------------------------------------------ the minimum window

    [Theory]
    [InlineData(RegistriesPanelViewModel.EntriesTab)]
    [InlineData(RegistriesPanelViewModel.ApprovedTab)]
    public void At_the_minimum_window_the_table_keeps_its_room_and_its_columns_fit(string tab)
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            scene.ViewModel.ActiveTab = tab;
            scene.Host.Relayout();

            var grid = scene.TabGrid;
            Assert.True(grid.ActualHeight >= 120, $"the {tab} table is {grid.ActualHeight:0} DIPs tall");
            Assert.True(VisualTree.Find<ScrollViewer>(grid)!.ScrollableWidth < 1, $"the {tab} table scrolls sideways by {VisualTree.Find<ScrollViewer>(grid)!.ScrollableWidth:0} DIPs");
            Assert.All(grid.Columns, c => Assert.True(c.ActualWidth >= c.MinWidth - 0.5, $"{c.Header} is {c.ActualWidth:0} DIPs wide"));

            // The toolbar still holds the title, the strip and the actions without pushing any off.
            var toolbar = scene.Toolbar;
            Assert.True(toolbar.ActualWidth <= scene.Shell.PageSize.Width + 1);
            Assert.True(scene.TabStrip.ActualWidth > 100);

            RenderTo.Png(scene.Host, $"registries-{tab}-tab-940x620");
        });
    }

    [Fact]
    public void At_the_minimum_window_the_sources_block_keeps_the_entries_room_with_the_extra_detail_lines()
    {
        using var scene = Scene.Open(940, 620);

        UiThread.Run(() =>
        {
            // Publisher, fetched at and the counts are in the card now; the entries of the source still get their room.
            var entries = scene.SourceEntriesGrid;
            Assert.True(entries.ActualHeight >= 150, $"the source's entries are {entries.ActualHeight:0} DIPs tall");
            Assert.True(scene.CountChips.IsVisible);
            Assert.True(scene.CountChips.ActualWidth > 0);

            RenderTo.Png(scene.Host, "registries-sources-detail-940x620");
        });
    }

    [Theory]
    [InlineData("Default", "Dark", "default-dark")]
    [InlineData("Cisco", "Light", "cisco-light")]
    public void Every_tab_is_drawn_in_two_looks_with_its_table_and_its_details(string styleName, string modeName, string look)
    {
        using var fixture = new AppearanceFixture(new AppearanceSettings(Enum.Parse<AppearanceStyle>(styleName), Enum.Parse<AppearanceMode>(modeName)));
        using var scene = Scene.Open(1400, 900);

        UiThread.Run(() =>
        {
            // Sources: the details card with the publisher, the fetch time and the chips.
            Assert.True(scene.Block("MasterDetail").IsVisible);
            Assert.True(scene.CountChips.IsVisible);
            RenderTo.Png(scene.Host, $"registries-sources-{look}-1400x900");

            foreach (var tab in new[] { RegistriesPanelViewModel.EntriesTab, RegistriesPanelViewModel.ApprovedTab })
            {
                scene.ViewModel.ActiveTab = tab;
                scene.Host.Relayout();
                Assert.True(scene.TabGrid.IsVisible);
                Assert.True(scene.TabGrid.ActualHeight > 100);
                RenderTo.Png(scene.Host, $"registries-{tab}-{look}-1400x900");
            }
        });
    }

    // ------------------------------------------------------------------ the scene

    private sealed class Scene : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly AppServices _services;

        private Scene(int width, int height, bool withSources, bool secondSource, string firstIndex, int extraEntries)
        {
            WriteIndex("corp-skills", PayloadFixtures.Read(firstIndex));
            if (secondSource)
            {
                var second = PayloadFixtures.Read("registry-index.second-source.json");
                if (extraEntries > 0)
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(second)!;
                    var verdicts = node["verdicts"]!.AsArray();
                    for (var i = 0; i < extraEntries; i++)
                    {
                        verdicts.Add(System.Text.Json.Nodes.JsonNode.Parse($"{{\"name\": \"bulk-{i}\", \"type\": \"skill\", \"status\": \"clean\", \"approved\": false, \"rejected\": false}}"));
                    }

                    second = node.ToJsonString();
                }

                WriteIndex("team.catalog", second);
            }

            _services = TestServices.Create(_temp);
            Shell = UiThread.Run(() =>
            {
                var shell = new PanelShell(_services, width, height);
                _ = shell.Show<RegistriesPanel>();
                return shell;
            });
            ViewModel = UiThread.Run(() => (RegistriesPanelViewModel)Shell.ViewModel);

            // The isolated services have no CLI, so the panel's own read ends at "not found"; settle that, then put the synthetic sources in front of it.
            UiThread.WaitFor(() => ViewModel.HasLoaded && !ViewModel.IsLoading, "registries read finished");
            if (!withSources)
            {
                UiThread.Run(() =>
                {
                    ViewModel.CliErrorMessage = null;
                    ViewModel.Trust.BeginRead();
                    ViewModel.Trust.MarkComplete();
                    ViewModel.NotifyTrust();
                    Host.Relayout();
                });
                return;
            }

            AddSources();
        }

        public PanelShell Shell { get; }

        public RegistriesPanelViewModel ViewModel { get; }

        public OffscreenHost Host => Shell.Host;

        public RegistriesPanel View => (RegistriesPanel)Shell.Page!;

        public DcPageToolbar Toolbar => VisualTree.Find<DcPageToolbar>(Shell.Page!)!;

        public DcSegmented TabStrip => VisualTree.Find<DcSegmented>(Shell.Page!, s => AutomationProperties.GetName(s) == "Registries view")
            ?? throw new InvalidOperationException("The tab strip was not built.");

        public Wpf.Ui.Controls.DataGrid TabGrid => Grid("Registry entries");

        public Wpf.Ui.Controls.DataGrid SourceEntriesGrid => Grid("Cached entries of the selected source");

        public FrameworkElement CountChips => (FrameworkElement)Shell.Page!.FindName("CountChips");

        public FrameworkElement Empty => (FrameworkElement)Shell.Page!.FindName("TabEmpty");

        public FrameworkElement Block(string name) => (FrameworkElement)Shell.Page!.FindName(name);

        public string Text(string name) => ((TextBlock)Shell.Page!.FindName(name)).Text;

        private Wpf.Ui.Controls.DataGrid Grid(string name) =>
            VisualTree.Find<Wpf.Ui.Controls.DataGrid>(Shell.Page!, g => AutomationProperties.GetName(g) == name)
            ?? throw new InvalidOperationException($"The '{name}' grid was not built.");

        /// <summary>The names of the toolbar's actions that are on screen, in order.</summary>
        public IEnumerable<string> VisibleToolbarActions() =>
            VisualTree.Descendants<Wpf.Ui.Controls.Button>(Toolbar).Where(b => b.IsVisible).Select(b => AutomationProperties.GetName(b));

        /// <summary>A button of the table's action bar, by its label.</summary>
        public Wpf.Ui.Controls.Button ActionButton(string label) =>
            VisualTree.Find<Wpf.Ui.Controls.Button>(Block("EntriesBlock"), b => b.Content as string == label)
            ?? throw new InvalidOperationException($"No '{label}' button.");

        /// <summary>Puts the synthetic sources in front of the panel and reads their caches, as a successful read of <c>registry list</c> would.</summary>
        public void AddSources()
        {
            Task? entries = null;
            UiThread.Run(() =>
            {
                ViewModel.CliErrorMessage = null;
                ViewModel.Trust.BeginRead();
                ViewModel.Trust.MarkComplete(); // the stand-in for a successful read: the actions in these tests are meant to work
                ViewModel.NotifyTrust();
                ViewModel.Sources.Add(Source("corp-skills"));
                ViewModel.Sources.Add(Source("team.catalog")); // listed whether or not it has a cache
                ViewModel.HasSources = true;
                ViewModel.SelectedSource = ViewModel.Sources.First(s => s.Id == "corp-skills");
                entries = ViewModel.LoadAllEntriesAsync();
            });
            UiThread.WaitFor(() => entries!.IsCompleted && !ViewModel.IsEntriesLoading && ViewModel.SourceFacts.HasCounts, "the caches were read");
            UiThread.Run(() => Host.Relayout());
        }

        private static RegistrySourceRow Source(string id) => new()
        {
            Id = id,
            Kind = "http_yaml",
            Content = "both",
            Enabled = true,
            LastSync = "2026-09-20T15:04:05+00:00",
            LastStatus = "ok",
            Location = $"https://registry.example.test/{id}.yaml",
            Fields = new[] { new RegistryFieldRow("id", id) },
        };

        private void WriteIndex(string sourceId, string json)
        {
            var folder = Path.Combine(_temp.Path, "registries", sourceId);
            _ = Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.json"), json);
        }

        /// <param name="secondSource">False: team.catalog is listed but has no cache.</param>
        /// <param name="firstIndex">The fixture corp-skills' cache is written from.</param>
        /// <param name="extraEntries">How many more skills (bulk-0 ..) team.catalog lists, to make the table long.</param>
        public static Scene Open(
            int width,
            int height,
            bool withSources = true,
            bool secondSource = true,
            string firstIndex = "registry-index.synced.json",
            int extraEntries = 0) =>
            new(width, height, withSources, secondSource, firstIndex, extraEntries);

        public void Dispose()
        {
            UiThread.Run(Shell.Dispose);
            _services.Dispose();
            _temp.Dispose();
        }
    }
}
